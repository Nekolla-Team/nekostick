using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Nekolla.Nekostick.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddExtensionContentAndNodeState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                schema: "nekostick",
                table: "extension_records",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "extension_node_states",
                schema: "nekostick",
                columns: table => new
                {
                    node_id = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    extension_record_id = table.Column<Guid>(type: "uuid", nullable: false),
                    observed_content_hash = table.Column<string>(type: "text", nullable: true),
                    load_state = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure_code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false, defaultValue: "None"),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamptz", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_extension_node_states", x => new { x.node_id, x.extension_record_id });
                    table.CheckConstraint(
                        "ck_extension_node_states_state",
                        "load_state IN ('Discovered', 'Loaded', 'Stopped', 'Failed', 'Unloading', 'Disabled') AND length(failure_code) BETWEEN 1 AND 64");
                    table.ForeignKey(
                        name: "fk_extension_node_states_nodes_node_id",
                        column: x => x.node_id,
                        principalSchema: "nekostick",
                        principalTable: "nodes",
                        principalColumn: "node_id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_extension_node_states_extension_records_extension_record_id",
                        column: x => x.extension_record_id,
                        principalSchema: "nekostick",
                        principalTable: "extension_records",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_extension_node_states_extension_record_id",
                schema: "nekostick",
                table: "extension_node_states",
                column: "extension_record_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "extension_node_states",
                schema: "nekostick");

            migrationBuilder.DropColumn(
                name: "content_hash",
                schema: "nekostick",
                table: "extension_records");
        }
    }
}
