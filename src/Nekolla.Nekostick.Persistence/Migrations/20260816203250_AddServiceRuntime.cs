using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddServiceRuntime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "service_runtimes",
                schema: "nekostick",
                columns: table => new
                {
                    service_id = table.Column<Guid>(type: "uuid", nullable: false),
                    node_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    lifecycle = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    health = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    restart_count = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false),
                    version = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_service_runtimes", x => new { x.node_id, x.service_id });
                    table.CheckConstraint("ck_service_runtimes_state", "lifecycle IN ('Disabled', 'Starting', 'Running', 'Stopping', 'Failed') AND health IN ('Unknown', 'Healthy', 'Unhealthy') AND restart_count >= 0");
                    table.ForeignKey(
                        name: "fk_service_runtimes_services_service_id",
                        column: x => x.service_id,
                        principalSchema: "nekostick",
                        principalTable: "services",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_service_runtimes_service_id",
                schema: "nekostick",
                table: "service_runtimes",
                column: "service_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "service_runtimes",
                schema: "nekostick");
        }
    }
}
